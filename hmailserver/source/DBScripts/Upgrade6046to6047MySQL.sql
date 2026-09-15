create table hm_remotedomainpolicies
(
	policyid int auto_increment not null, primary key(`policyid`), unique(`policyid`),
	policydomainname varchar(255) not null,
	policydescription varchar(255) not null,
	policyactive tinyint not null,
	policyoutboundtls int not null,
	policyinboundtls tinyint not null,
	policymaxmessagesizekb int not null,
	policymaxconnections int not null,
	policymaxperminute int not null,
	policyallowreplies tinyint not null,
	policyallowforwarding tinyint not null,
	policycalloutenabled tinyint not null,
	policycallouthost varchar(255) not null,
	policycalloutport int not null,
	policycallouttimeout int not null,
	policycalloutcacheminutes int not null,
	policycalloutperminute int not null
);

CREATE INDEX idx_hm_remotedomainpolicies_domain ON hm_remotedomainpolicies (policydomainname);

ALTER TABLE hm_remotedomainpolicies ENGINE=InnoDB;

update hm_dbversion set value = 6047;
