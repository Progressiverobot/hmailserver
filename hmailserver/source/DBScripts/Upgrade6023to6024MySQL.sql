alter table hm_domains add column domainvacationmessageon int not null default 0;

alter table hm_domains add column domainvacationsubject varchar(200) not null default '';

alter table hm_domains add column domainvacationmessage text;

alter table hm_domains add column domainvacationinternalsubject varchar(200) not null default '';

alter table hm_domains add column domainvacationinternalmessage text;

alter table hm_domains add column domainvacationexternaloverride int not null default 0;

create table hm_blocked_senders
(
	bsid bigint auto_increment not null, primary key(bsid), unique(bsid),
	bsaddress varchar(255) not null,
	bsscore int not null,
	bsdescription varchar(255) not null,
	unique u_bsaddress (bsaddress)
) DEFAULT CHARSET=utf8;

update hm_dbversion set value = 6024;
