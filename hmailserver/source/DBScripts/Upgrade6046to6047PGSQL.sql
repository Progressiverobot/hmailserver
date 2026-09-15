create table hm_remotedomainpolicies
(
	policyid bigserial not null primary key,
	policydomainname varchar(255) not null,
	policydescription varchar(255) not null,
	policyactive smallint not null,
	policyoutboundtls int not null,
	policyinboundtls smallint not null,
	policymaxmessagesizekb int not null,
	policymaxconnections int not null,
	policymaxperminute int not null,
	policyallowreplies smallint not null,
	policyallowforwarding smallint not null,
	policycalloutenabled smallint not null,
	policycallouthost varchar(255) not null,
	policycalloutport int not null,
	policycallouttimeout int not null,
	policycalloutcacheminutes int not null,
	policycalloutperminute int not null
);

CREATE INDEX idx_hm_remotedomainpolicies_domain ON hm_remotedomainpolicies (policydomainname);

update hm_dbversion set value = 6047;
